using System;
using System.Collections.Generic;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public class GraphicsSettingsService : IGraphicsSettingsService
    {
        public string? ReadIniValue(string filePath, string key)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                string[] lines = File.ReadAllLines(filePath);

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";"))
                        continue;

                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex > 0)
                    {
                        string lineKey = line.Substring(0, equalsIndex).Trim();
                        if (lineKey == key)
                        {
                            return line.Substring(equalsIndex + 1).Trim();
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private void ModifyIniFile(string filePath, Dictionary<string, string> replacements)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"INI file not found: {filePath}");
            }

            FileInfo fileInfo = new FileInfo(filePath);

            if (fileInfo.IsReadOnly)
                fileInfo.IsReadOnly = false;

            try
            {
                string[] lines = File.ReadAllLines(filePath);
                var modifiedKeys = new HashSet<string>();

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";"))
                        continue;

                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex > 0)
                    {
                        string key = line.Substring(0, equalsIndex).Trim();
                        if (replacements.ContainsKey(key))
                        {
                            lines[i] = replacements[key];
                            modifiedKeys.Add(key);
                        }
                    }
                }

                var missingKeys = new List<string>();
                foreach (var key in replacements.Keys)
                {
                    if (!modifiedKeys.Contains(key))
                    {
                        missingKeys.Add(key);
                    }
                }

                if (missingKeys.Count > 0)
                {
                    int systemSettingsIndex = -1;
                    int nextSectionIndex = -1;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string trimmedLine = lines[i].Trim();
                        if (trimmedLine.Equals("[SystemSettings]", StringComparison.OrdinalIgnoreCase))
                        {
                            systemSettingsIndex = i;
                        }
                        else if (systemSettingsIndex >= 0 && trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                        {
                            nextSectionIndex = i;
                            break;
                        }
                    }

                    if (systemSettingsIndex >= 0)
                    {
                        int insertIndex = nextSectionIndex >= 0 ? nextSectionIndex : lines.Length;
                        var newLines = new List<string>(lines);

                        foreach (var key in missingKeys)
                        {
                            newLines.Insert(insertIndex, replacements[key]);
                            insertIndex++;
                        }

                        lines = newLines.ToArray();
                    }
                }

                File.WriteAllLines(filePath, lines);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public void ApplyVSync(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "UseVsync", $"UseVsync={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyAntiAliasing(string tdEngineIniPath, string level)
        {
            var replacements = new Dictionary<string, string>();

            switch (level)
            {
                case "Off":
                    replacements["MaxMultisamples"] = "MaxMultisamples=1";
                    break;
                case "2x":
                    replacements["MaxMultisamples"] = "MaxMultisamples=2";
                    break;
                case "4x":
                    replacements["MaxMultisamples"] = "MaxMultisamples=4";
                    break;
                case "8x":
                    replacements["MaxMultisamples"] = "MaxMultisamples=8";
                    break;
                case "8xQ":
                    replacements["MaxMultisamples"] = "MaxMultisamples=10";
                    break;
                case "16xQ":
                    replacements["MaxMultisamples"] = "MaxMultisamples=12";
                    break;
            }

            if (replacements.Count > 0)
            {
                ModifyIniFile(tdEngineIniPath, replacements);
            }
        }

        public void ApplyAnisotropicFiltering(string tdEngineIniPath, string level)
        {
            var replacements = new Dictionary<string, string>();

            switch (level)
            {
                case "Off":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=0";
                    break;
                case "2x":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=2";
                    break;
                case "4x":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=4";
                    break;
                case "8x":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=8";
                    break;
                case "16x":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=16";
                    break;
            }

            if (replacements.Count > 0)
            {
                ModifyIniFile(tdEngineIniPath, replacements);
            }
        }

        public void ApplyPhysX(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "PhysXEnhanced", $"PhysXEnhanced={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyRenderResolution(string tdEngineIniPath, int percentage)
        {
            bool upscale = percentage != 100;

            var replacements = new Dictionary<string, string>
            {
                { "ScreenPercentage", $"ScreenPercentage={percentage}.0" },
                { "UpscaleScreenPercentage", $"UpscaleScreenPercentage={upscale.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyTextureDetailPreset(string tdEngineIniPath, string preset)
        {
            var replacements = GetTextureDetailReplacements(preset);
            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyGraphicsQualityPreset(string tdEngineIniPath, string preset)
        {
            var replacements = GetGraphicsQualityReplacements(preset);
            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyStaticDecals(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "StaticDecals", $"StaticDecals={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyDynamicDecals(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "DynamicDecals", $"DynamicDecals={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyRadialBlur(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "TdMotionBlur", $"TdMotionBlur={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyBloomAndDoF(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "Bloom", $"Bloom={enabled.ToString()}" },
                { "DepthOfField", $"DepthOfField={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyLensFlare(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "LensFlares", $"LensFlares={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyDynamicLights(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "DynamicLights", $"DynamicLights={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyDynamicShadows(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "DynamicShadows", $"DynamicShadows={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyHQDynamicShadows(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>();

            if (enabled)
            {
                replacements["bEnableVSMShadows"] = "bEnableVSMShadows=False";
                replacements["bEnableBranchingPCFShadows"] = "bEnableBranchingPCFShadows=True";
                replacements["ShadowFilterRadius"] = "ShadowFilterRadius=4";
                replacements["ModShadowFadeDistanceExponent"] = "ModShadowFadeDistanceExponent=0";
                replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=16";
                replacements["MaxShadowResolution"] = "MaxShadowResolution=2048";
            }
            else
            {
                replacements["bEnableVSMShadows"] = "bEnableVSMShadows=True";
                replacements["bEnableBranchingPCFShadows"] = "bEnableBranchingPCFShadows=False";
                replacements["ShadowFilterRadius"] = "ShadowFilterRadius=2";
                replacements["ModShadowFadeDistanceExponent"] = "ModShadowFadeDistanceExponent=.2";
                replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=2";
                replacements["MaxShadowResolution"] = "MaxShadowResolution=1024";
            }

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyLightmaps(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "DirectionalLightmaps", $"DirectionalLightmaps={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplySunHaze(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "TdSunHaze", $"TdSunHaze={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyToneMapping(string tdEngineIniPath, bool enabled)
        {
            var replacements = new Dictionary<string, string>
            {
                { "TdTonemapping", $"TdTonemapping={enabled.ToString()}" }
            };

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        public void ApplyTextureManagement(string tdEngineIniPath, string mode)
        {
            var replacements = new Dictionary<string, string>();

            if (mode == "Modern")
            {
                replacements["PoolSize"] = "PoolSize=0";
                replacements["OnlyStreamInTextures"] = "OnlyStreamInTextures=True";
            }
            else
            {
                replacements["PoolSize"] = "PoolSize=250";
                replacements["OnlyStreamInTextures"] = "OnlyStreamInTextures=False";
            }

            ModifyIniFile(tdEngineIniPath, replacements);
        }

        private Dictionary<string, string> GetTextureDetailReplacements(string preset)
        {
            var replacements = new Dictionary<string, string>();

            // common settings for all texture detail presets
            replacements["bEnableVSMShadows"] = "bEnableVSMShadows=True";
            replacements["bEnableBranchingPCFShadows"] = "bEnableBranchingPCFShadows=False";
            replacements["ModShadowFadeDistanceExponent"] = "ModShadowFadeDistanceExponent=.2";
            replacements["PoolSize"] = "PoolSize=250";
            replacements["StaticDecals"] = "StaticDecals=True";
            replacements["DynamicDecals"] = "DynamicDecals=True";
            replacements["DynamicLights"] = "DynamicLights=True";
            replacements["DynamicShadows"] = "DynamicShadows=True";
            replacements["DirectionalLightmaps"] = "DirectionalLightmaps=True";
            replacements["OnlyStreamInTextures"] = "OnlyStreamInTextures=False";
            replacements["TdTonemapping"] = "TdTonemapping=True";
            replacements["SkeletalMeshLODBias"] = "SkeletalMeshLODBias=0";
            replacements["ParticleLODBias"] = "ParticleLODBias=0";

            switch (preset)
            {
                case "Lowest":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=2";
                    replacements["SceneCaptureStreamingMultiplier"] = "SceneCaptureStreamingMultiplier=0.800000";
                    replacements["FoliageDrawRadiusMultiplier"] = "FoliageDrawRadiusMultiplier=0.000000";
                    replacements["TEXTUREGROUP_World"] = "TEXTUREGROUP_World=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_WorldNormalMap"] = "TEXTUREGROUP_WorldNormalMap=(MinLODSize=256,MaxLODSize=256,LODBias=2)";
                    replacements["TEXTUREGROUP_WorldSpecular"] = "TEXTUREGROUP_WorldSpecular=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_Character"] = "TEXTUREGROUP_Character=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_CharacterNormalMap"] = "TEXTUREGROUP_CharacterNormalMap=(MinLODSize=256,MaxLODSize=256,LODBias=2)";
                    replacements["TEXTUREGROUP_CharacterSpecular"] = "TEXTUREGROUP_CharacterSpecular=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_Weapon"] = "TEXTUREGROUP_Weapon=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_WeaponNormalMap"] = "TEXTUREGROUP_WeaponNormalMap=(MinLODSize=256,MaxLODSize=256,LODBias=2)";
                    replacements["TEXTUREGROUP_WeaponSpecular"] = "TEXTUREGROUP_WeaponSpecular=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_Vehicle"] = "TEXTUREGROUP_Vehicle=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_VehicleNormalMap"] = "TEXTUREGROUP_VehicleNormalMap=(MinLODSize=256,MaxLODSize=256,LODBias=2)";
                    replacements["TEXTUREGROUP_VehicleSpecular"] = "TEXTUREGROUP_VehicleSpecular=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_Cinematic"] = "TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Effects"] = "TEXTUREGROUP_Effects=(MinLODSize=256,MaxLODSize=256,LODBias=1)";
                    replacements["TEXTUREGROUP_Skybox"] = "TEXTUREGROUP_Skybox=(MinLODSize=256,MaxLODSize=512,LODBias=1)";
                    replacements["TEXTUREGROUP_UI"] = "TEXTUREGROUP_UI=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_LightAndShadowMap"] = "TEXTUREGROUP_LightAndShadowMap=(MinLODSize=256,MaxLODSize=512,LODBias=1)";
                    replacements["TEXTUREGROUP_RenderTarget"] = "TEXTUREGROUP_RenderTarget=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TdBicubicFiltering"] = "TdBicubicFiltering=False";
                    break;

                case "Low":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=2";
                    replacements["SceneCaptureStreamingMultiplier"] = "SceneCaptureStreamingMultiplier=0.900000";
                    replacements["FoliageDrawRadiusMultiplier"] = "FoliageDrawRadiusMultiplier=0.000000";
                    replacements["TEXTUREGROUP_World"] = "TEXTUREGROUP_World=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_WorldNormalMap"] = "TEXTUREGROUP_WorldNormalMap=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_WorldSpecular"] = "TEXTUREGROUP_WorldSpecular=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_Character"] = "TEXTUREGROUP_Character=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_CharacterNormalMap"] = "TEXTUREGROUP_CharacterNormalMap=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_CharacterSpecular"] = "TEXTUREGROUP_CharacterSpecular=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_Weapon"] = "TEXTUREGROUP_Weapon=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_WeaponNormalMap"] = "TEXTUREGROUP_WeaponNormalMap=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_WeaponSpecular"] = "TEXTUREGROUP_WeaponSpecular=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_Vehicle"] = "TEXTUREGROUP_Vehicle=(MinLODSize=256,MaxLODSize=2048,LODBias=1)";
                    replacements["TEXTUREGROUP_VehicleNormalMap"] = "TEXTUREGROUP_VehicleNormalMap=(MinLODSize=256,MaxLODSize=2048,LODBias=1)";
                    replacements["TEXTUREGROUP_VehicleSpecular"] = "TEXTUREGROUP_VehicleSpecular=(MinLODSize=256,MaxLODSize=2048,LODBias=1)";
                    replacements["TEXTUREGROUP_Cinematic"] = "TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Effects"] = "TEXTUREGROUP_Effects=(MinLODSize=256,MaxLODSize=1024,LODBias=1)";
                    replacements["TEXTUREGROUP_Skybox"] = "TEXTUREGROUP_Skybox=(MinLODSize=512,MaxLODSize=2048,LODBias=1)";
                    replacements["TEXTUREGROUP_UI"] = "TEXTUREGROUP_UI=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_LightAndShadowMap"] = "TEXTUREGROUP_LightAndShadowMap=(MinLODSize=512,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_RenderTarget"] = "TEXTUREGROUP_RenderTarget=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TdBicubicFiltering"] = "TdBicubicFiltering=False";
                    break;

                case "Medium":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=4";
                    replacements["SceneCaptureStreamingMultiplier"] = "SceneCaptureStreamingMultiplier=1.000000";
                    replacements["FoliageDrawRadiusMultiplier"] = "FoliageDrawRadiusMultiplier=0.500000";
                    replacements["TEXTUREGROUP_World"] = "TEXTUREGROUP_World=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_WorldNormalMap"] = "TEXTUREGROUP_WorldNormalMap=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_WorldSpecular"] = "TEXTUREGROUP_WorldSpecular=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_Character"] = "TEXTUREGROUP_Character=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_CharacterNormalMap"] = "TEXTUREGROUP_CharacterNormalMap=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_CharacterSpecular"] = "TEXTUREGROUP_CharacterSpecular=(MinLODSize=256,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_Weapon"] = "TEXTUREGROUP_Weapon=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_WeaponNormalMap"] = "TEXTUREGROUP_WeaponNormalMap=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_WeaponSpecular"] = "TEXTUREGROUP_WeaponSpecular=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_Vehicle"] = "TEXTUREGROUP_Vehicle=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_VehicleNormalMap"] = "TEXTUREGROUP_VehicleNormalMap=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_VehicleSpecular"] = "TEXTUREGROUP_VehicleSpecular=(MinLODSize=1,MaxLODSize=4096,LODBias=1)";
                    replacements["TEXTUREGROUP_Cinematic"] = "TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Effects"] = "TEXTUREGROUP_Effects=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Skybox"] = "TEXTUREGROUP_Skybox=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_UI"] = "TEXTUREGROUP_UI=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_LightAndShadowMap"] = "TEXTUREGROUP_LightAndShadowMap=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_RenderTarget"] = "TEXTUREGROUP_RenderTarget=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TdBicubicFiltering"] = "TdBicubicFiltering=False";
                    break;

                case "High":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=4";
                    replacements["SceneCaptureStreamingMultiplier"] = "SceneCaptureStreamingMultiplier=1.000000";
                    replacements["FoliageDrawRadiusMultiplier"] = "FoliageDrawRadiusMultiplier=1.000000";
                    replacements["TEXTUREGROUP_World"] = "TEXTUREGROUP_World=(MinLODSize=256,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_WorldNormalMap"] = "TEXTUREGROUP_WorldNormalMap=(MinLODSize=512,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_WorldSpecular"] = "TEXTUREGROUP_WorldSpecular=(MinLODSize=256,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_Character"] = "TEXTUREGROUP_Character=(MinLODSize=512,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_CharacterNormalMap"] = "TEXTUREGROUP_CharacterNormalMap=(MinLODSize=512,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_CharacterSpecular"] = "TEXTUREGROUP_CharacterSpecular=(MinLODSize=512,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_Weapon"] = "TEXTUREGROUP_Weapon=(MinLODSize=512,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_WeaponNormalMap"] = "TEXTUREGROUP_WeaponNormalMap=(MinLODSize=1024,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_WeaponSpecular"] = "TEXTUREGROUP_WeaponSpecular=(MinLODSize=512,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_Vehicle"] = "TEXTUREGROUP_Vehicle=(MinLODSize=1024,MaxLODSize=2048,LODBias=0)";
                    replacements["TEXTUREGROUP_VehicleNormalMap"] = "TEXTUREGROUP_VehicleNormalMap=(MinLODSize=1024,MaxLODSize=2048,LODBias=0)";
                    replacements["TEXTUREGROUP_VehicleSpecular"] = "TEXTUREGROUP_VehicleSpecular=(MinLODSize=1024,MaxLODSize=2048,LODBias=0)";
                    replacements["TEXTUREGROUP_Cinematic"] = "TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Effects"] = "TEXTUREGROUP_Effects=(MinLODSize=256,MaxLODSize=1024,LODBias=0)";
                    replacements["TEXTUREGROUP_Skybox"] = "TEXTUREGROUP_Skybox=(MinLODSize=512,MaxLODSize=2048,LODBias=0)";
                    replacements["TEXTUREGROUP_UI"] = "TEXTUREGROUP_UI=(MinLODSize=1024,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_LightAndShadowMap"] = "TEXTUREGROUP_LightAndShadowMap=(MinLODSize=512,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_RenderTarget"] = "TEXTUREGROUP_RenderTarget=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TdBicubicFiltering"] = "TdBicubicFiltering=True";
                    break;

                case "Highest":
                    replacements["MaxAnisotropy"] = "MaxAnisotropy=16";
                    replacements["SceneCaptureStreamingMultiplier"] = "SceneCaptureStreamingMultiplier=1.000000";
                    replacements["FoliageDrawRadiusMultiplier"] = "FoliageDrawRadiusMultiplier=1.000000";
                    replacements["TEXTUREGROUP_World"] = "TEXTUREGROUP_World=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_WorldNormalMap"] = "TEXTUREGROUP_WorldNormalMap=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_WorldSpecular"] = "TEXTUREGROUP_WorldSpecular=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Character"] = "TEXTUREGROUP_Character=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_CharacterNormalMap"] = "TEXTUREGROUP_CharacterNormalMap=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_CharacterSpecular"] = "TEXTUREGROUP_CharacterSpecular=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Weapon"] = "TEXTUREGROUP_Weapon=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_WeaponNormalMap"] = "TEXTUREGROUP_WeaponNormalMap=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_WeaponSpecular"] = "TEXTUREGROUP_WeaponSpecular=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Vehicle"] = "TEXTUREGROUP_Vehicle=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_VehicleNormalMap"] = "TEXTUREGROUP_VehicleNormalMap=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_VehicleSpecular"] = "TEXTUREGROUP_VehicleSpecular=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Cinematic"] = "TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Effects"] = "TEXTUREGROUP_Effects=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_Skybox"] = "TEXTUREGROUP_Skybox=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_UI"] = "TEXTUREGROUP_UI=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_LightAndShadowMap"] = "TEXTUREGROUP_LightAndShadowMap=(MinLODSize=256,MaxLODSize=4096,LODBias=0)";
                    replacements["TEXTUREGROUP_RenderTarget"] = "TEXTUREGROUP_RenderTarget=(MinLODSize=1,MaxLODSize=4096,LODBias=0)";
                    replacements["TdBicubicFiltering"] = "TdBicubicFiltering=True";
                    break;
            }

            return replacements;
        }

        private Dictionary<string, string> GetGraphicsQualityReplacements(string preset)
        {
            var replacements = new Dictionary<string, string>();

            // common settings for all graphics quality presets
            replacements["bEnableVSMShadows"] = "bEnableVSMShadows=True";
            replacements["bEnableBranchingPCFShadows"] = "bEnableBranchingPCFShadows=False";
            replacements["ModShadowFadeDistanceExponent"] = "ModShadowFadeDistanceExponent=.2";
            replacements["PoolSize"] = "PoolSize=250";
            replacements["StaticDecals"] = "StaticDecals=True";
            replacements["DynamicDecals"] = "DynamicDecals=True";
            replacements["DynamicLights"] = "DynamicLights=True";
            replacements["DynamicShadows"] = "DynamicShadows=True";
            replacements["DirectionalLightmaps"] = "DirectionalLightmaps=True";
            replacements["OnlyStreamInTextures"] = "OnlyStreamInTextures=False";
            replacements["TdTonemapping"] = "TdTonemapping=True";
            replacements["SkeletalMeshLODBias"] = "SkeletalMeshLODBias=0";
            replacements["ParticleLODBias"] = "ParticleLODBias=0";

            switch (preset)
            {
                case "Lowest":
                    replacements["DepthOfField"] = "DepthOfField=False";
                    replacements["Bloom"] = "Bloom=False";
                    replacements["QualityBloom"] = "QualityBloom=False";
                    replacements["Distortion"] = "Distortion=False";
                    replacements["LensFlares"] = "LensFlares=False";
                    replacements["EnableHighPolyChars"] = "EnableHighPolyChars=False";
                    replacements["DetailMode"] = "DetailMode=0";
                    replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=-1";
                    replacements["MaxShadowResolution"] = "MaxShadowResolution=256";
                    replacements["ShadowTexelsPerPixel"] = "ShadowTexelsPerPixel=1.000000";
                    replacements["TdMotionBlur"] = "TdMotionBlur=False";
                    replacements["TdSunHaze"] = "TdSunHaze=False";
                    break;

                case "Low":
                    replacements["DepthOfField"] = "DepthOfField=True";
                    replacements["Bloom"] = "Bloom=True";
                    replacements["QualityBloom"] = "QualityBloom=False";
                    replacements["Distortion"] = "Distortion=True";
                    replacements["LensFlares"] = "LensFlares=True";
                    replacements["EnableHighPolyChars"] = "EnableHighPolyChars=False";
                    replacements["DetailMode"] = "DetailMode=1";
                    replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=0";
                    replacements["MaxShadowResolution"] = "MaxShadowResolution=512";
                    replacements["ShadowTexelsPerPixel"] = "ShadowTexelsPerPixel=2.000000";
                    replacements["TdMotionBlur"] = "TdMotionBlur=False";
                    replacements["TdSunHaze"] = "TdSunHaze=False";
                    break;

                case "Medium":
                    replacements["DepthOfField"] = "DepthOfField=True";
                    replacements["Bloom"] = "Bloom=True";
                    replacements["QualityBloom"] = "QualityBloom=False";
                    replacements["Distortion"] = "Distortion=True";
                    replacements["LensFlares"] = "LensFlares=True";
                    replacements["EnableHighPolyChars"] = "EnableHighPolyChars=False";
                    replacements["DetailMode"] = "DetailMode=2";
                    replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=0";
                    replacements["MaxShadowResolution"] = "MaxShadowResolution=1024";
                    replacements["ShadowTexelsPerPixel"] = "ShadowTexelsPerPixel=2.000000";
                    replacements["TdMotionBlur"] = "TdMotionBlur=True";
                    replacements["TdSunHaze"] = "TdSunHaze=True";
                    break;

                case "High":
                    replacements["DepthOfField"] = "DepthOfField=True";
                    replacements["Bloom"] = "Bloom=True";
                    replacements["QualityBloom"] = "QualityBloom=False";
                    replacements["Distortion"] = "Distortion=True";
                    replacements["LensFlares"] = "LensFlares=True";
                    replacements["EnableHighPolyChars"] = "EnableHighPolyChars=False";
                    replacements["DetailMode"] = "DetailMode=2";
                    replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=1";
                    replacements["MaxShadowResolution"] = "MaxShadowResolution=1024";
                    replacements["ShadowTexelsPerPixel"] = "ShadowTexelsPerPixel=2.000000";
                    replacements["TdMotionBlur"] = "TdMotionBlur=True";
                    replacements["TdSunHaze"] = "TdSunHaze=True";
                    break;

                case "Highest":
                    replacements["DepthOfField"] = "DepthOfField=True";
                    replacements["Bloom"] = "Bloom=True";
                    replacements["QualityBloom"] = "QualityBloom=True";
                    replacements["Distortion"] = "Distortion=True";
                    replacements["LensFlares"] = "LensFlares=True";
                    replacements["EnableHighPolyChars"] = "EnableHighPolyChars=True";
                    replacements["DetailMode"] = "DetailMode=3";
                    replacements["ShadowFilterQualityBias"] = "ShadowFilterQualityBias=2";
                    replacements["MaxShadowResolution"] = "MaxShadowResolution=1024";
                    replacements["ShadowTexelsPerPixel"] = "ShadowTexelsPerPixel=2.000000";
                    replacements["TdMotionBlur"] = "TdMotionBlur=True";
                    replacements["TdSunHaze"] = "TdSunHaze=True";
                    break;
            }

            return replacements;
        }

        public string DetectTextureDetailPreset(string tdEngineIniPath)
        {
            string? textureManagement = ReadIniValue(tdEngineIniPath, "OnlyStreamInTextures");
            if (textureManagement != null && textureManagement.Equals("True", StringComparison.OrdinalIgnoreCase))
            {
                return "Custom";
            }

            string[] textureSpecificKeys =
            {
                "MaxAnisotropy",
                "SceneCaptureStreamingMultiplier",
                "FoliageDrawRadiusMultiplier",
                "TEXTUREGROUP_World",
                "TEXTUREGROUP_WorldNormalMap",
                "TEXTUREGROUP_WorldSpecular",
                "TEXTUREGROUP_Character",
                "TEXTUREGROUP_CharacterNormalMap",
                "TEXTUREGROUP_CharacterSpecular",
                "TEXTUREGROUP_Weapon",
                "TEXTUREGROUP_WeaponNormalMap",
                "TEXTUREGROUP_WeaponSpecular",
                "TEXTUREGROUP_Vehicle",
                "TEXTUREGROUP_VehicleNormalMap",
                "TEXTUREGROUP_VehicleSpecular",
                "TEXTUREGROUP_Cinematic",
                "TEXTUREGROUP_Effects",
                "TEXTUREGROUP_Skybox",
                "TEXTUREGROUP_UI",
                "TEXTUREGROUP_LightAndShadowMap",
                "TEXTUREGROUP_RenderTarget",
                "TdBicubicFiltering"
            };

            string[] presets = { "Lowest", "Low", "Medium", "High", "Highest" };

            foreach (string preset in presets)
            {
                var expectedValues = GetTextureDetailReplacements(preset);
                bool matches = true;

                foreach (var kvp in expectedValues)
                {
                    if (!textureSpecificKeys.Contains(kvp.Key))
                        continue;

                    string? actualValue = ReadIniValue(tdEngineIniPath, kvp.Key);
                    string expectedValue = kvp.Value.Substring(kvp.Value.IndexOf('=') + 1);

                    if (actualValue != expectedValue)
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return preset;
                }
            }

            return "Custom";
        }

        public string DetectGraphicsQualityPreset(string tdEngineIniPath)
        {
            string[] qualitySpecificKeys =
            {
                "DepthOfField",
                "Bloom",
                "QualityBloom",
                "Distortion",
                "LensFlares",
                "EnableHighPolyChars",
                "DetailMode",
                "ShadowFilterQualityBias",
                "MaxShadowResolution",
                "ShadowTexelsPerPixel",
                "TdMotionBlur",
                "TdSunHaze"
            };

            string[] presets = { "Lowest", "Low", "Medium", "High", "Highest" };

            foreach (string preset in presets)
            {
                var expectedValues = GetGraphicsQualityReplacements(preset);
                bool matches = true;

                foreach (var kvp in expectedValues)
                {
                    if (!qualitySpecificKeys.Contains(kvp.Key))
                        continue;

                    string? actualValue = ReadIniValue(tdEngineIniPath, kvp.Key);
                    string expectedValue = kvp.Value.Substring(kvp.Value.IndexOf('=') + 1);

                    if (actualValue != expectedValue)
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return preset;
                }
            }

            return "Custom";
        }

        public void ApplyMinLOD(string tdEngineIniPath, int value)
        {
            if (!File.Exists(tdEngineIniPath))
            {
                throw new FileNotFoundException($"INI file not found: {tdEngineIniPath}");
            }

            FileInfo fileInfo = new FileInfo(tdEngineIniPath);
            if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

            try
            {
                string fileData = File.ReadAllText(tdEngineIniPath);

                fileData = System.Text.RegularExpressions.Regex.Replace(
                    fileData,
                    @"(MinLODSize=)(\d+)",
                    "${1}" + value);

                File.WriteAllText(tdEngineIniPath, fileData);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public void ApplyMaxLOD(string tdEngineIniPath, int value)
        {
            if (!File.Exists(tdEngineIniPath))
            {
                throw new FileNotFoundException($"INI file not found: {tdEngineIniPath}");
            }

            FileInfo fileInfo = new FileInfo(tdEngineIniPath);
            if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

            try
            {
                string fileData = File.ReadAllText(tdEngineIniPath);

                fileData = System.Text.RegularExpressions.Regex.Replace(
                    fileData,
                    @"(MaxLODSize=)(\d+)",
                    "${1}" + value);

                File.WriteAllText(tdEngineIniPath, fileData);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public void ApplyLODBias(string tdEngineIniPath, int value)
        {
            if (!File.Exists(tdEngineIniPath))
            {
                throw new FileNotFoundException($"INI file not found: {tdEngineIniPath}");
            }

            FileInfo fileInfo = new FileInfo(tdEngineIniPath);
            if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

            try
            {
                string fileData = File.ReadAllText(tdEngineIniPath);

                fileData = System.Text.RegularExpressions.Regex.Replace(
                    fileData,
                    @"(LODBias=)(-?\d+)",
                    "${1}" + value);

                File.WriteAllText(tdEngineIniPath, fileData);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public void ApplyStreakEffect(string defaultHudEffectsIniPath, bool enabled)
        {
            if (!File.Exists(defaultHudEffectsIniPath))
            {
                throw new FileNotFoundException($"INI file not found: {defaultHudEffectsIniPath}");
            }

            FileInfo fileInfo = new FileInfo(defaultHudEffectsIniPath);
            if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

            try
            {
                var replacementMap = new Dictionary<string, string>();

                if (enabled)
                {
                    replacementMap["bEnableStreakEffect"] = "bEnableStreakEffect=true";
                    replacementMap["StreakDistanceInMovementDirection"] = "StreakDistanceInMovementDirection=120";
                    replacementMap["StreakDistanceInCameraDirection"] = "StreakDistanceInCameraDirection=120";
                    replacementMap["StreakEffectFadeTime"] = "StreakEffectFadeTime=0.34f";
                }
                else
                {
                    replacementMap["bEnableStreakEffect"] = "bEnableStreakEffect=false";
                    replacementMap["StreakDistanceInMovementDirection"] = "StreakDistanceInMovementDirection=0";
                    replacementMap["StreakDistanceInCameraDirection"] = "StreakDistanceInCameraDirection=0";
                    replacementMap["StreakEffectFadeTime"] = "StreakEffectFadeTime=0.00f";
                }

                string[] lines = File.ReadAllLines(defaultHudEffectsIniPath);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains('='))
                        continue;

                    string key = line.Split('=')[0];
                    if (replacementMap.ContainsKey(key))
                    {
                        lines[i] = replacementMap[key];
                    }
                }

                File.WriteAllLines(defaultHudEffectsIniPath, lines);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public string? ReadStreakEffectStatus(string defaultHudEffectsIniPath)
        {
            if (!File.Exists(defaultHudEffectsIniPath))
            {
                return null;
            }

            try
            {
                string[] lines = File.ReadAllLines(defaultHudEffectsIniPath);

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(";"))
                        continue;

                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex > 0)
                    {
                        string lineKey = line.Substring(0, equalsIndex).Trim();
                        if (lineKey == "bEnableStreakEffect")
                        {
                            string value = line.Substring(equalsIndex + 1).Trim();
                            return value;
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public void ApplyFPSLimit(string tdEngineIniPath, int fpsValue)
        {
            if (!File.Exists(tdEngineIniPath))
            {
                throw new FileNotFoundException($"INI file not found: {tdEngineIniPath}");
            }

            FileInfo fileInfo = new FileInfo(tdEngineIniPath);
            if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

            try
            {
                string[] lines = File.ReadAllLines(tdEngineIniPath);
                bool foundSmoothFrameRate = false;
                bool foundMaxSmoothedFrameRate = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("bSmoothFrameRate="))
                    {
                        lines[i] = "bSmoothFrameRate=TRUE";
                        foundSmoothFrameRate = true;
                    }
                    else if (lines[i].StartsWith("MaxSmoothedFrameRate="))
                    {
                        lines[i] = $"MaxSmoothedFrameRate={fpsValue}";
                        foundMaxSmoothedFrameRate = true;
                    }
                }

                if (!foundSmoothFrameRate || !foundMaxSmoothedFrameRate)
                {
                    throw new Exception("'TdEngine.ini' file is corrupted or missing required keys.");
                }

                File.WriteAllLines(tdEngineIniPath, lines);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public void RemoveFPSLimit(string tdEngineIniPath)
        {
            if (!File.Exists(tdEngineIniPath))
            {
                throw new FileNotFoundException($"INI file not found: {tdEngineIniPath}");
            }

            FileInfo fileInfo = new FileInfo(tdEngineIniPath);
            if (fileInfo.IsReadOnly) fileInfo.IsReadOnly = false;

            try
            {
                string[] lines = File.ReadAllLines(tdEngineIniPath);
                bool foundSmoothFrameRate = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].StartsWith("bSmoothFrameRate="))
                    {
                        lines[i] = "bSmoothFrameRate=FALSE";
                        foundSmoothFrameRate = true;
                        break;
                    }
                }

                if (!foundSmoothFrameRate)
                {
                    throw new Exception("'TdEngine.ini' file is corrupted or missing required keys.");
                }

                File.WriteAllLines(tdEngineIniPath, lines);
            }
            finally
            {
                fileInfo.IsReadOnly = true;
            }
        }

        public (bool isLimited, int? fpsValue) ReadFPSLimitStatus(string tdEngineIniPath)
        {
            if (!File.Exists(tdEngineIniPath))
            {
                return (false, null);
            }

            try
            {
                string[] lines = File.ReadAllLines(tdEngineIniPath);
                bool? smoothFrameRate = null;
                int? maxSmoothedFrameRate = null;

                foreach (string line in lines)
                {
                    if (line.StartsWith("bSmoothFrameRate="))
                    {
                        string value = line.Substring("bSmoothFrameRate=".Length).Trim();
                        smoothFrameRate = value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (line.StartsWith("MaxSmoothedFrameRate="))
                    {
                        string value = line.Substring("MaxSmoothedFrameRate=".Length).Trim();
                        if (int.TryParse(value, out int fps))
                        {
                            maxSmoothedFrameRate = fps;
                        }
                    }
                }

                bool isLimited = smoothFrameRate == true;
                return (isLimited, maxSmoothedFrameRate);
            }
            catch
            {
                return (false, null);
            }
        }
    }
}

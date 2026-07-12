using CommunityToolkit.Mvvm.Input;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Info-dialog texts for the Graphics Tweaks tab. A single keyed command replaces the 28
    // one-line ShowXInfo commands; XAML buttons bind ShowInfoCommand with a CommandParameter key.
    public partial class GraphicsTweaksViewModel
    {
        [RelayCommand]
        private void ShowInfo(string key)
        {
            if (InfoTexts.TryGetValue(key, out var info))
            {
                _dialogService.ShowMessage(info.Title, info.Message, DialogMessageType.Information);
            }
        }

        private static readonly Dictionary<string, (string Title, string Message)> InfoTexts = new()
        {
            ["Fov"] = ("FOV Information",
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
                "as the baseline. Weapon zoom sensitivity still tracks the zoomed FOV as normal. Also useful for TAS tools."),

            ["HighResFix"] = ("Resolution Information",
                "Mirror's Edge accepts only the resolutions currently available in your system's display settings. However, it is possible to use other software " +
                "(e.g. Custom Resolution Utility, NVIDIA Control Panel, etc.) to add custom display resolutions. Once these are configured, they will appear here.\n\n" +
                "Selecting a resolution will also apply the following fixes:\n\n" +
                "• Removes the hardcoded 16:9 aspect ratio constraint, allowing the game to render correctly at any aspect ratio without letterboxing/pillarboxing.\n\n" +
                "• Applies a render target fix to prevent the white-screen issue at narrower aspect ratios above 720p (e.g. Steam Deck's native resolution).\n\n" +
                "• Enables dynamic FOV scaling so the game automatically applies HOR+ correction at aspect ratios wider than 16:9, and VERT+ at narrower ratios.\n\n" +
                "• Compensates cutscene zoom rates, vertigo effects, and unzoom timing to work correctly and consistently at any FOV and aspect ratio.\n\n" +
                "Selecting a resolution with a horizontal pixel count greater than 1920 will also prompt you with the option to fix the blurry in-game text and other UI fixes. " +
                "Once applied, this fix remains dynamic and further in-game resolution adjustments will self-apply the high-res fix."),

            ["VSync"] = ("VSync Information",
                "Vertical Sync synchronises the frame rate with your monitor's refresh rate to prevent screen tearing. Enabling it may increase input latency."),

            ["FpsLimit"] = ("FPS Limit Information",
                "Default = FPS limit of 62.\n\n60-62 FPS limit is a requirement for speedruns to be verified, any other setting is banned. " +
                "Speedrunning strategies become increasingly more difficult as FPS increases, therefore it is not advised to deviate from the 60-62 FPS limit.\n\n" +
                "As framerate increases, so does player friction which can alter the speed of certain movement mechanics and make forced slides more difficult to control " +
                "as framerates exceed 150 FPS (i.e. Chapter 1C RP&A building slide). Enemy accuracy is also increased at higher framerates. " +
                "Additionally, as load times are tied to framerate, loading times decrease as framerate increases. These effects are otherwise generally not noticeable to casual players " +
                "and the game can be comfortably played with a higher FPS limit in place.\n\nIf you want to run the game with no FPS limiter at all, click the 'Remove Limit' button."),

            ["AntiAliasing"] = ("Anti-Aliasing Information",
                "Anti-aliasing smooths jagged edges in the game. Higher values provide better quality but reduce performance."),

            ["PhysX"] = ("PhysX Information",
                "PhysX provides additional physics effects such as detailed debris and cloth simulations, and spawns in extra physics props.\n\n" +
                "Note: PhysX in Mirror's Edge is hardware accelerated only on CUDA-ready NVIDIA GPUs."),

            ["PhysXFps"] = ("PhysX FPS Information",
                "Applies a PhysX FPS value to cloth simulations (flags, construction tarps, strip curtain doors, etc.). Accepts a minimum of 50 FPS and a maximum of 300 FPS. No effect if PhysX is disabled.\n\n" +
                "Cloth simulations in Mirror's Edge are simulated at a rate independent of the game's framerate, otherwise known as time-steps. By default, Mirror's Edge uses a value of 50 FPS " +
                "for PhysX cloth simulations, which can appear choppy when using reaction time or when running the game above the 62 FPS limit.\n\n" +
                "Suggestions:\n\n• If playing at the default 62 FPS limit, change the PhysX FPS value to 62 FPS to match the simulation rate with the game's framerate. " +
                "This effectively removes the frame pacing appearance of PhysX cloth.\n\n• If playing at uncapped FPS, set this value to whatever you want (max of 300 FPS)."),

            ["RenderResolution"] = ("Render Resolution Information",
                "Controls the internal rendering resolution relative to your display output.\n\n" +
                "Below 100%: Renders at a lower resolution and upscales, improving performance on lower-end systems.\n\n" +
                "Above 100%: Renders at a higher resolution and downscales to your display, producing sharper visuals " +
                "with reduced aliasing. Setting 200% renders at 4x pixel density."),

            ["TextureDetail"] = ("Texture Detail Information",
                "Texture detail controls the resolution/LODs of textures, as well as the level of anisotropic filtering and bicubic filtering to be applied.\n\nThis setting mirrors the in-game video options."),

            ["GraphicsQuality"] = ("Graphics Quality Information",
                "Graphics quality controls mesh/shadow quality, as well as various other post-process effects such as bloom, depth of field, lens flares, etc.\n\nThis setting mirrors the in-game video options."),

            ["AnisotropicFiltering"] = ("Anisotropic Filtering Information",
                "Anisotropic filtering improves texture quality when viewed at oblique angles. Higher values provide better quality."),

            ["StaticDecals"] = ("Static Decals Information",
                "Static decals are pre-placed decals (runner glyphs, paint/graffiti, etc.)."),

            ["DynamicDecals"] = ("Dynamic Decals Information",
                "Dynamic decals are decals spawned during gameplay (typically bullet holes and explosion effects)."),

            ["RadialBlur"] = ("Radial Blur Information",
                "Radial blur is the blurring applied to the edges of the screen when running. It is seperate from the streak effect."),

            ["StreakEffect"] = ("Streak Effect Information",
                "When approaching top running speed, streak effects will appear on the edges of the screen which can become more noticeable at higher FOV settings. " +
                "\n\nDisabling requires the 'Unlocked Configs' patch in the 'Game Tweaks' section."),

            ["BloomDoF"] = ("Bloom & DoF Information",
                "Bloom creates a glow effect around bright lights. Depth of Field blurs objects that are out of focus." +
                "\n\nThe shaders involved for rendering Bloom and Depth of Field are dependent on each other and cannot be individually toggled on/off."),

            ["LensFlare"] = ("Lens Flare Information",
                "Allows enabling/disabling the lens flares emitted from the sun and various light sources. In some maps this will also remove the appearance of the sun altogether."),

            ["DynamicLights"] = ("Dynamic Lights Information",
                "Dynamic lights are any light sources that dynamically illuminate the scene and characters. Typical examples include flashlights/cop car lights and ambient character illumination."),

            ["DynamicShadows"] = ("Dynamic Shadows Information",
                "Dynamic shadows are the modulated shadows casted onto the environment from characters. This also includes self-shadowing of characters."),

            ["HqDynamicShadows"] = ("HQ Dynamic Shadows Information",
                "High Quality dynamic shadows doubles the resolution of what's available from the \"Highest\" graphics quality preset, " +
                "forces the maximum shadow resolution to always be shown, increases the filtering quality, and disables VSM shadowing in favour of the superior-quality PCF shadowing." +
                "\n\nNote: \"High quality\" dynamic shadows will have no effect if dynamic shadows are disabled."),

            ["Lightmaps"] = ("Lightmaps Information",
                "Light maps are the pre-baked lighting used to globally illuminate the environment. These light maps can be disabled (for most objects), " +
                "showing the original textures without the environment's GI and shadow contributions. Note that disabling can also make some vertex-baked objects appear black."),

            ["SunHaze"] = ("Sun Haze Information",
                "Toggles the appearance of atmospheric haze around the sun. This haze can bleed through buildings in some scenarios."),

            ["ToneMapping"] = ("Tone Mapping Information",
                "Tone mapping adjusts the post-process exposure/colour curves, which are applied on a per-map basis. " +
                "Disabling tone mapping typically makes the image appear brighter and with less contrast."),

            ["TextureManagement"] = ("Texture Management Information",
                "The \"Modern\" setting removes the 250MB VRAM allocation limit to textures and forces textures to remain in the texture pool once loaded. " +
                "This can resolve the random blurry texture bug, and assists with large custom maps that don't utilise level streaming.\n\n" +
                "If you have a low-end system, it may be more preferable to keep this setting to \"Default\"."),

            ["MinLod"] = ("Minimum LOD Information",
                "Minimum LOD size controls the lowest quality texture mipmap that will be loaded. Range: 1-4096 (Unreal Engine 3 has a max limit of 4096)."),

            ["MaxLod"] = ("Maximum LOD Information",
                "Maximum LOD size controls the highest quality texture mipmap that will be loaded. Range: 1-4096 (Unreal Engine 3 has a max limit of 4096)."),

            ["LodBias"] = ("LOD Bias Information",
                "Adjusts the distance at which different texture mipmaps are loaded. A higher bias value results in lower resolution texture mipmaps being shown sooner " +
                "as the player moves away from the texture surface and vice versa. A minimum bias of 0 (highest quality, shows only the maximum resolution LOD) " +
                "and a maximum bias of 12 (lowest quality) can be entered."),

            ["ToneMapper"] = ("Tone Mapper Information",
                "Replaces the game's post-process tone mapping shaders. Selecting an option downloads and installs the corresponding shader files.\n\n" +
                "• Original — The game's default tone mapping shaders.\n\n" +
                "• Faithful Luma — A luminance-preserving tone mapper that better retains highlight detail and colour, while also " +
                "fixing the black floor level, providing more accurate bloom handling and making the auto-exposure system much more responsive.\n\n" +
                "Note: Neither tone mapping option will have an effect if the 'Tone Mapping' toggle in the Individual Settings section above is disabled."),
        };
    }
}

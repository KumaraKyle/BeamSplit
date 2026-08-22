using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BeamSplit.Core;

/// <summary>
/// Edits a BeamNG profile's settings files.
///
/// The four focus-related settings here are not optional for splitscreen. BeamNG's
/// shipped defaults (settings\defaults.json) penalise any window that isn't focused,
/// and BeamSplit deliberately keeps every game window unfocused:
///
///   AudioMuteOnWindowLoseFocus  cloud  true   -> only one instance would have sound
///   unfocusedInput              cloud  false  -> unfocused instances ignore input
///   fpsLimitBackgroundEnabled   local  true   -> background instances get throttled
///   fpsLimitBackground          local  30     -> ...to 30fps
///
/// The cloud/local tag in defaults.json says which file each one lives in.
/// BeamNG rewrites these on exit, so they are applied before every launch.
/// </summary>
public static class GameSettings
{
    internal static double AudioMasterLevel(AppConfig cfg, int instance) =>
        string.Equals(cfg.AudioMixMode, "P0Only", StringComparison.OrdinalIgnoreCase) && instance > 0
            ? 0d
            : Math.Clamp(cfg.AudioMaster, 0, 100) / 100d;

    private static string CloudFile(AppConfig cfg, int i) =>
        Path.Combine(Instances.CurrentProfile(cfg, i), "settings", "cloud", "settings.json");

    private static string LocalFile(AppConfig cfg, int i) =>
        Path.Combine(Instances.CurrentProfile(cfg, i), "settings", "settings.json");

    public static void ApplyFocusFixes(AppConfig cfg, int i, IProgress<string>? log = null)
    {
        Patch(CloudFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["unfocusedInput"] = true
        });
        Patch(LocalFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["fpsLimitEnabled"] = true,
            ["fpsLimit"] = Math.Clamp(cfg.FrameLimit, 30, 240),
            ["fpsLimitBackgroundEnabled"] = true,
            ["fpsLimitBackground"] = Math.Clamp(cfg.FrameLimit, 30, 240),
            ["GraphicDisplayModes"] = "Window"
        });
        log?.Report($"  P{i}: windowed, input in background, {Math.Clamp(cfg.FrameLimit, 30, 240)} fps cap");
    }

    /// <summary>Applies the shared audio mix and output device to one profile.</summary>
    public static void ApplyAudio(AppConfig cfg, int i, IProgress<string>? log = null)
    {
        static double Level(int percent) => Math.Clamp(percent, 0, 100) / 100d;

        Patch(CloudFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["AudioMuteOnWindowLoseFocus"] = !cfg.AudioInBackground
        });

        var primaryMixMuted = string.Equals(cfg.AudioMixMode, "P0Only", StringComparison.OrdinalIgnoreCase) && i > 0;
        var master = AudioMasterLevel(cfg, i);
        var effects = Level(cfg.AudioEffects);
        Patch(LocalFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["AudioMasterVol"] = master,
            ["AudioMusicVol"] = Level(cfg.AudioMusic),
            ["AudioUiVol"] = Level(cfg.AudioUi),
            ["AudioPowerVol"] = effects,
            ["AudioForcedInductionVol"] = effects,
            ["AudioTransmissionVol"] = effects,
            ["AudioSuspensionVol"] = effects,
            ["AudioSurfaceVol"] = effects,
            ["AudioCollisionVol"] = effects,
            ["AudioAeroVol"] = effects,
            ["AudioEnvironmentVol"] = effects,
            ["AudioOtherVol"] = effects,
            ["AudioEnableStereoHeadphones"] = cfg.AudioStereoHeadphones,
            ["AudioDevice"] = cfg.AudioDevice ?? ""
        });

        var device = string.IsNullOrWhiteSpace(cfg.AudioDevice) ? "Windows default" : cfg.AudioDevice;
        var mix = primaryMixMuted ? "muted (P0 supplies shared mix)" : $"{Math.Clamp(cfg.AudioMaster, 0, 100)}%";
        log?.Report($"  P{i}: audio {mix}, effects {Math.Clamp(cfg.AudioEffects, 0, 100)}%, output {device}, background {(cfg.AudioInBackground ? "on" : "muted")}");
    }

    public static void ApplyGraphics(AppConfig cfg, int i, IProgress<string>? log = null)
    {
        if (cfg.LowMemoryGraphics)
        {
            // BeamNG 0.39's own Lowest preset. Texture=Lowest caps textures at quarter
            // resolution, the largest documented VRAM/streaming reduction. Keeping the
            // complete preset together avoids expensive features being silently left on.
            Patch(LocalFile(cfg, i), new Dictionary<string, JsonNode?>
            {
                ["GraphicOverallQuality"] = "Custom",
                ["GraphicMeshQuality"] = "Lowest",
                ["GraphicTerrainQuality"] = "Lowest",
                ["GraphicTextureQuality"] = "Lowest",
                ["GraphicLightingQuality"] = "Lowest",
                ["GraphicShadowsQuality"] = "Lowest",
                ["lastSplitCastersEnabled"] = false,
                ["vehicleShadowEnabled"] = false,
                ["GraphicMaxDecalCount"] = 1000,
                ["GraphicGrassDensity"] = 0,
                ["GraphicAnisotropic"] = 0,
                ["GraphicAntialias"] = 0,
                ["GraphicAntialiasType"] = "fxaa",
                ["GraphicDynReflectionEnabled"] = false,
                ["GraphicDynReflectionFacesPerupdate"] = 1,
                ["GraphicDynReflectionDetail"] = 0,
                ["GraphicDynReflectionDistance"] = 10,
                ["GraphicDynReflectionTexsize"] = 0,
                ["GraphicDynMirrorsEnabled"] = false,
                ["GraphicDynMirrorsDetail"] = 0,
                ["GraphicDynMirrorsDistance"] = 10,
                ["GraphicDynMirrorsTexsize"] = 0,
                ["GraphicCloudsQuality"] = "Disabled",
                ["GraphicClusteredQuality"] = "Lowest",
                ["PostFXSSAOGeneralEnabled"] = false,
                ["PostFXScreenSpaceShadowsEnabled"] = false,
                ["PostFXDOFGeneralEnabled"] = false,
                ["PostFXMotionBlurEnabled"] = false,
                ["SkipGenerateLicencePlate"] = true,
                ["uiAcceleratedRender"] = false
            });
            log?.Report($"  P{i}: LOW-MEMORY graphics (quarter textures, lowest world detail, reflections/grass/post-FX off)");
            return;
        }
        if (!cfg.ApplyGraphics) return;
        Patch(LocalFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["GraphicAnisotropic"] = cfg.Aniso,
            ["GraphicAntialias"] = cfg.AntiAlias,
            ["GraphicAntialiasType"] = cfg.AntiAlias <= 1 ? "fxaa" : "msaa",
            ["GraphicDisableShadows"] = cfg.NoShadows ? "1" : "0"
        });
        log?.Report($"  P{i}: graphics applied (aniso {cfg.Aniso}, AA {cfg.AntiAlias}, shadows {(cfg.NoShadows ? "off" : "on")})");
    }

    /// <summary>
    /// Tells this instance's game which launcher port to talk to.
    /// MPCoreNetwork.lua reads it: settings.getValue("launcherPort", 4444)
    /// </summary>
    public static void SetLauncherPort(AppConfig cfg, int i, int port)
    {
        Patch(LocalFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["launcherPort"] = port,
            ["launcherIp"] = "127.0.0.1"
        });
    }

    /// <summary>Fresh profiles register a newly downloaded mod DISABLED - flip it on.</summary>
    public static void EnableBeamMpMod(AppConfig cfg, int i)
    {
        var db = Path.Combine(Instances.CurrentProfile(cfg, i), "mods", "db.json");
        if (!File.Exists(db)) return;
        try
        {
            var text = File.ReadAllText(db);
            if (text.Contains("\"active\":false"))
                File.WriteAllText(db, text.Replace("\"active\":false", "\"active\":true"), new UTF8Encoding(false));
        }
        catch { }
    }

    /// <summary>Merges keys into a BeamNG settings json, creating it if needed.</summary>
    private static void Patch(string path, Dictionary<string, JsonNode?> values)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            JsonObject obj;
            if (File.Exists(path))
            {
                try { obj = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? []; }
                catch { obj = []; }
            }
            else obj = [];

            foreach (var (k, v) in values) obj[k] = v?.DeepClone();

            File.WriteAllText(path,
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* a settings write failing shouldn't abort a launch */ }
    }
}

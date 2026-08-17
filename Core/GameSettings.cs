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
    private static string CloudFile(AppConfig cfg, int i) =>
        Path.Combine(Instances.CurrentProfile(cfg, i), "settings", "cloud", "settings.json");

    private static string LocalFile(AppConfig cfg, int i) =>
        Path.Combine(Instances.CurrentProfile(cfg, i), "settings", "settings.json");

    public static void ApplyFocusFixes(AppConfig cfg, int i, IProgress<string>? log = null)
    {
        Patch(CloudFile(cfg, i), new Dictionary<string, JsonNode?>
        {
            ["AudioMuteOnWindowLoseFocus"] = false,
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
        log?.Report($"  P{i}: windowed, audio/input in background, {Math.Clamp(cfg.FrameLimit, 30, 240)} fps cap");
    }

    public static void ApplyGraphics(AppConfig cfg, int i, IProgress<string>? log = null)
    {
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

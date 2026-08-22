using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace BeamSplit.Core;

public sealed record SingleInstanceCapability(bool Supported, string Detail);

public readonly record struct SingleInstanceLayout(Rect Window, IReadOnlyList<Rect> Viewports);

/// <summary>Capability gating, layout manifest, and mod deployment for the Lua-only engine.</summary>
public static class SingleInstanceSupport
{
    public const int SchemaVersion = 1;
    public const string ModFileName = "beamsplit_single_instance.zip";

    public static SingleInstanceCapability CheckCapability(AppConfig cfg)
    {
        if (!Detect.IsGameRoot(cfg.GameRoot))
            return new(false, "BeamNG.drive was not found.");

        var root = cfg.GameRoot!;
        var camera = Path.Combine(root, "lua", "ge", "extensions", "core", "camera.lua");
        var gameEngine = Path.Combine(root, "lua", "ge", "extensions", "core", "cameraModes", "gameengine.lua");
        var bindings = Path.Combine(root, "lua", "ge", "extensions", "core", "input", "bindings.lua");
        var hud = Path.Combine(root, "ui", "ui-vue", "src", "modules", "splitscreen", "SplitScreenHud.vue");

        foreach (var file in new[] { camera, gameEngine, bindings, hud })
            if (!File.Exists(file))
                return new(false, $"This BeamNG build is missing {Path.GetFileName(file)}.");

        try
        {
            if (!File.ReadAllText(camera).Contains("M.createContext = createContext", StringComparison.Ordinal) ||
                !File.ReadAllText(gameEngine).Contains("updateRenderView", StringComparison.Ordinal) ||
                !File.ReadAllText(bindings).Contains("M.setPlayerToDevice = setPlayerToDevice", StringComparison.Ordinal))
                return new(false, "This BeamNG build does not expose the required camera or multiseat APIs.");
        }
        catch (Exception ex) { return new(false, $"Could not inspect BeamNG capabilities: {ex.Message}"); }

        return new(true, "Compatible camera contexts, render views, multiseat routing, and split-screen HUD found.");
    }

    public static SingleInstanceLayout ResolveLayout(AppConfig cfg, IReadOnlyList<MonitorInfo> monitors)
    {
        if (cfg.Players.Count != 2) throw new InvalidOperationException("Single-instance mode requires exactly two players.");
        if (monitors.Count == 0) throw new InvalidOperationException("No Windows displays were detected.");
        var regions = cfg.Players.OrderBy(p => p.Index).Select(p => Tiling.RegionFor(p, monitors)).ToList();
        var left = regions.Min(r => r.X);
        var top = regions.Min(r => r.Y);
        var right = regions.Max(r => r.X + r.W);
        var bottom = regions.Max(r => r.Y + r.H);
        var window = new Rect(left, top, right - left, bottom - top);
        var local = regions.Select(r => new Rect(r.X - left, r.Y - top, r.W, r.H)).ToList();
        return new(window, local);
    }

    public static string Deploy(AppConfig cfg, IProgress<string>? log = null)
    {
        var capability = CheckCapability(cfg);
        if (!capability.Supported) throw new InvalidOperationException(capability.Detail);

        var profile = Instances.CurrentProfile(cfg, Instances.SingleInstanceIndex);
        var mods = Path.Combine(profile, "mods");
        var settings = Path.Combine(profile, "settings", "beamsplit");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(settings);

        var zipPath = Path.Combine(mods, ModFileName);
        BuildModZip(zipPath);
        var manifestPath = Path.Combine(settings, "session.json");
        File.WriteAllText(manifestPath, BuildManifest(cfg), new UTF8Encoding(false));
        log?.Report($"  single-instance mod and session manifest deployed to {profile}");
        return manifestPath;
    }

    internal static string BuildManifest(AppConfig cfg, IReadOnlyList<MonitorInfo>? monitorOverride = null)
    {
        var monitors = monitorOverride ?? Native.GetMonitors();
        var layout = ResolveLayout(cfg, monitors);
        var ordered = cfg.Players.OrderBy(p => p.Index).ToList();
        var players = ordered.Select((p, i) =>
        {
            var r = layout.Viewports[i];
            var device = p.Keyboard ? "keyboard0" : p.Pad >= 0 ? $"xinput{p.Pad}" : null;
            return new
            {
                index = i,
                player = i,
                device,
                keyboard = p.Keyboard,
                label = p.Keyboard ? "Keyboard + mouse" : p.Pad >= 0 ? $"Controller {p.Pad + 1}" : "Unassigned",
                monitorDevice = p.MonitorDevice,
                rect = new[] { r.X / (double)layout.Window.W, r.Y / (double)layout.Window.H,
                               r.W / (double)layout.Window.W, r.H / (double)layout.Window.H },
                pixelRect = new[] { r.X, r.Y, r.W, r.H }
            };
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            engine = "single-instance-experimental",
            windowBounds = new[] { layout.Window.X, layout.Window.Y, layout.Window.W, layout.Window.H },
            hud = true,
            camera = "orbit",
            players
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static void BuildModZip(string destination)
    {
        var temp = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                AddResource(archive, "BeamSplit.Resources.SingleInstance.modScript.lua", "scripts/BeamSplit/modScript.lua");
                AddResource(archive, "BeamSplit.Resources.SingleInstance.info.json", "mod_info/BeamSplit/info.json");
                AddResource(archive, "BeamSplit.Resources.SingleInstance.splitScreen.lua", "lua/ge/extensions/render/splitScreen.lua");
                AddResource(archive, "BeamSplit.Resources.SingleInstance.actions.json", "lua/ge/extensions/core/input/actions/zz_beamsplitSplitScreen.json");
            }
            File.Move(temp, destination, true);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static void AddResource(ZipArchive archive, string resourceName, string entryName)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource missing: {resourceName}");
        using var output = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        input.CopyTo(output);
    }

    internal static bool ResourcesAvailable()
    {
        var names = Assembly.GetExecutingAssembly().GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        return names.Contains("BeamSplit.Resources.SingleInstance.modScript.lua") &&
               names.Contains("BeamSplit.Resources.SingleInstance.info.json") &&
               names.Contains("BeamSplit.Resources.SingleInstance.splitScreen.lua") &&
               names.Contains("BeamSplit.Resources.SingleInstance.actions.json");
    }
}

using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace BeamSplit.Core;

/// <summary>
/// Adds BeamSplit's vehicle-Lua audio hook to the version-matched BeamMP client after
/// the launcher completes its update pass. BeamMP already loads every extension in
/// lua/vehicle/extensions/BeamMP and marks vehicles L (local) or R (remote).
/// </summary>
public static class BeamMpAudioIsolation
{
    public const string EntryName = "lua/vehicle/extensions/BeamMP/BeamSplitAudioVE.lua";
    private const string ResourceName = "BeamSplit.Resources.BeamSplitAudioVE.lua";

    public static bool ResourceAvailable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is { Length: > 100 };
    }

    public static void PatchClient(AppConfig cfg, int instance, bool enabled,
        IProgress<string>? log = null)
    {
        var zip = Path.Combine(Instances.CurrentProfile(cfg, instance), "mods", "multiplayer", "BeamMP.zip");
        if (!File.Exists(zip)) return;
        try
        {
            PatchZip(zip, enabled);
            log?.Report(enabled
                ? $"  P{instance}: remote BeamMP vehicle audio suppressed; local perspective retained"
                : $"  P{instance}: full BeamMP world audio enabled");
        }
        catch (Exception ex)
        {
            log?.Report($"  P{instance}: remote-audio hook failed - {ex.Message}");
        }
    }

    internal static void PatchZip(string zipPath, bool enabled)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        archive.GetEntry(EntryName)?.Delete();
        if (!enabled) return;

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("embedded audio hook is missing");
        var entry = archive.CreateEntry(EntryName, CompressionLevel.Optimal);
        using var output = entry.Open();
        resource.CopyTo(output);
    }

    public static bool IsPatched(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.GetEntry(EntryName) is { Length: > 100 };
        }
        catch { return false; }
    }
}

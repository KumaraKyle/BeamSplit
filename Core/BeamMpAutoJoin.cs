using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace BeamSplit.Core;

/// <summary>
/// Adds a tiny event-driven extension to each instance's pinned BeamMP client. It uses
/// BeamMP's own public Lua API to request guest login and direct-connect to BeamSplit's
/// local server; no menu automation or changes to the real game install are involved.
/// </summary>
public static class BeamMpAutoJoin
{
    public const string EntryName = "lua/ge/extensions/BeamSplitAutoJoin.lua";
    private const string ModScriptEntry = "scripts/BeamMP/modScript.lua";
    private const string ResourceName = "BeamSplit.Resources.BeamSplitAutoJoin.lua";
    private const string PortToken = "__BEAMSPLIT_SERVER_PORT__";
    private const string BeginMarker = "-- BeamSplit auto-join begin";
    private const string EndMarker = "-- BeamSplit auto-join end";
    private const string LoaderBlock = """

-- BeamSplit auto-join begin
load("BeamSplitAutoJoin")
setExtensionUnloadMode("BeamSplitAutoJoin", "manual")
-- BeamSplit auto-join end
""";

    public static bool ResourceAvailable()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is { Length: > 300 };
    }

    public static void PatchClient(AppConfig cfg, int instance, bool enabled,
        IProgress<string>? log = null)
    {
        var zip = Path.Combine(Instances.CurrentProfile(cfg, instance), "mods", "multiplayer", "BeamMP.zip");
        if (!File.Exists(zip)) return;

        var port = ServerConfig.Port(cfg);
        try
        {
            PatchZip(zip, enabled, port);
            log?.Report(enabled
                ? $"  P{instance}: BeamMP guest auto-join armed for 127.0.0.1:{port}"
                : $"  P{instance}: BeamMP auto-join disabled");
        }
        catch (Exception ex)
        {
            log?.Report($"  P{instance}: BeamMP auto-join patch failed - {ex.Message}");
        }
    }

    internal static void PatchZip(string zipPath, bool enabled, int port)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        archive.GetEntry(EntryName)?.Delete();

        var modEntry = archive.GetEntry(ModScriptEntry)
            ?? throw new InvalidDataException($"BeamMP client has no {ModScriptEntry}");
        string modScript;
        using (var reader = new StreamReader(modEntry.Open())) modScript = reader.ReadToEnd();
        var cleanScript = RemoveLoaderBlock(modScript);

        if (enabled)
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("embedded BeamMP auto-join hook is missing");
            using var reader = new StreamReader(resource);
            var lua = reader.ReadToEnd().Replace(PortToken,
                Math.Clamp(port, 1, 65535).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var autoJoin = archive.CreateEntry(EntryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(autoJoin.Open());
            writer.Write(lua);
            cleanScript = cleanScript.TrimEnd() + LoaderBlock;
        }

        if (!string.Equals(modScript, cleanScript, StringComparison.Ordinal))
        {
            modEntry.Delete();
            var replacement = archive.CreateEntry(ModScriptEntry, CompressionLevel.Optimal);
            using var writer = new StreamWriter(replacement.Open());
            writer.Write(cleanScript);
        }
    }

    internal static bool IsPatched(string zipPath, int port)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var hook = archive.GetEntry(EntryName);
            var mod = archive.GetEntry(ModScriptEntry);
            if (hook is null || mod is null) return false;
            using var hookReader = new StreamReader(hook.Open());
            using var modReader = new StreamReader(mod.Open());
            return hookReader.ReadToEnd().Contains($"local serverPort = {port}", StringComparison.Ordinal) &&
                   modReader.ReadToEnd().Contains(BeginMarker, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string RemoveLoaderBlock(string source)
    {
        while (true)
        {
            var begin = source.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (begin < 0) return source;
            var end = source.IndexOf(EndMarker, begin, StringComparison.Ordinal);
            if (end < 0) return source[..begin].TrimEnd() + Environment.NewLine;
            end += EndMarker.Length;
            while (end < source.Length && source[end] is '\r' or '\n') end++;
            source = source.Remove(begin, end - begin);
        }
    }
}

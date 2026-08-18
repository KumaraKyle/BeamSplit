using System.IO;

namespace BeamSplit.Core;

public sealed record ModPackage(string RelativePath, string FullPath, long Bytes)
{
    public string Name => Path.GetFileName(RelativePath);
}

/// <summary>
/// Synchronises user-selected ZIP mods into BeamSplit-owned locations. The user's
/// normal BeamNG mods folder is always read-only, and the pinned BeamMP.zip remains in
/// mods/multiplayer where neither this manager nor normal personal mods can replace it.
/// </summary>
public static class ModManager
{
    public const string PlayerFolderName = "beamsplit-shared";

    public static string? DetectDefaultSource()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var ini = Path.Combine(local, "BeamNG", "BeamNG.drive.ini");
        var userFolder = Path.Combine(local, "BeamNG", "BeamNG.drive", "current");

        try
        {
            if (File.Exists(ini))
            {
                foreach (var raw in File.ReadLines(ini))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("userFolder", StringComparison.OrdinalIgnoreCase)) continue;
                    var split = line.IndexOf('=');
                    if (split < 0) continue;
                    var value = line[(split + 1)..].Trim().Trim('"');
                    if (value.Length == 0) break;
                    userFolder = Path.IsPathRooted(value)
                        ? value
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ini)!, value));
                    break;
                }
            }
        }
        catch { }

        var mods = Path.Combine(userFolder, "mods");
        return Directory.Exists(mods) ? mods : null;
    }

    public static IReadOnlyList<ModPackage> Discover(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)) return [];
        var root = Path.GetFullPath(source);
        try
        {
            return Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories)
                .Select(path => new ModPackage(Path.GetRelativePath(root, path), path, new FileInfo(path).Length))
                // multiplayer contains downloads supplied by a server, including the
                // BeamMP client. Re-sharing those as personal/server mods causes loops.
                .Where(mod => !FirstPart(mod.RelativePath).Equals("multiplayer", StringComparison.OrdinalIgnoreCase))
                .OrderBy(mod => mod.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    public static void Apply(AppConfig cfg, int playerCount, IProgress<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(cfg.ModsSourceDir) || !Directory.Exists(cfg.ModsSourceDir))
        {
            if (cfg.ModsConfigured)
                log?.Report("Mods: source folder is unavailable; existing managed copies were left in place.");
            return;
        }
        var discovered = Discover(cfg.ModsSourceDir)
            .ToDictionary(m => Normalize(m.RelativePath), StringComparer.OrdinalIgnoreCase);
        SyncPlayers(cfg, Math.Max(0, playerCount), discovered, log);
        SyncServer(cfg, discovered, log);
    }

    private static void SyncPlayers(AppConfig cfg, int playerCount,
        IReadOnlyDictionary<string, ModPackage> discovered, IProgress<string>? log)
    {
        var selected = cfg.PlayerModFiles.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        for (var i = 0; i < playerCount; i++)
        {
            var target = Path.Combine(Instances.CurrentProfile(cfg, i), "mods", PlayerFolderName);
            CleanOwnedDirectory(target);
            if (!cfg.UsePlayerMods) continue;

            foreach (var relative in selected)
            {
                if (!discovered.TryGetValue(relative, out var mod)) continue;
                var destination = SafeDestination(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    CopyAtomic(mod.FullPath, destination);
                    copied++;
                }
                catch (Exception ex) { log?.Report($"Personal mods: could not copy {relative} to P{i} - {ex.Message}"); }
            }
        }
        log?.Report(cfg.UsePlayerMods
            ? $"Personal mods: synced {selected.Count} package(s) to {playerCount} player profile(s) ({copied} copies)."
            : "Personal mods: shared profile folder disabled and cleared.");
    }

    private static void SyncServer(AppConfig cfg, IReadOnlyDictionary<string, ModPackage> discovered,
        IProgress<string>? log)
    {
        if (string.IsNullOrWhiteSpace(cfg.ServerDir))
        {
            if (cfg.ServerModFiles.Count > 0) log?.Report("Server mods: no BeamMP server folder is configured yet.");
            return;
        }

        var client = Path.Combine(cfg.ServerDir, "Resources", "Client");
        Directory.CreateDirectory(client);
        var previouslyManaged = cfg.ManagedServerModFiles
            .Where(IsSimpleFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = cfg.ServerModFiles.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var old in previouslyManaged)
        {
            if (wanted.Any(relative => Path.GetFileName(relative).Equals(old, StringComparison.OrdinalIgnoreCase))) continue;
            TryDelete(Path.Combine(client, old), log);
        }

        var nowManaged = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in wanted.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!discovered.TryGetValue(relative, out var mod)) continue;
            var name = Path.GetFileName(relative);
            if (!usedNames.Add(name))
            {
                log?.Report($"Server mods: skipped {relative}; another selected ZIP is also named {name}.");
                continue;
            }

            var destination = Path.Combine(client, name);
            if (File.Exists(destination) && !previouslyManaged.Contains(name))
            {
                log?.Report($"Server mods: kept hand-installed {name}; deselect or remove it manually to let BeamSplit manage that name.");
                continue;
            }

            try
            {
                CopyAtomic(mod.FullPath, destination);
                nowManaged.Add(name);
            }
            catch (Exception ex)
            {
                log?.Report($"Server mods: could not copy {name} - {ex.Message}");
                if (previouslyManaged.Contains(name) && File.Exists(destination)) nowManaged.Add(name);
            }
        }

        cfg.ManagedServerModFiles = nowManaged;
        log?.Report($"Server mods: {nowManaged.Count} BeamSplit-managed package(s) in Resources\\Client." +
                    (ServerConfig.IsRunning() ? " Restart the server before players reconnect." : ""));
    }

    internal static string SafeDestination(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(fullRoot, Normalize(relative)));
        if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A mod path escaped its managed folder.");
        return destination;
    }

    private static string Normalize(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static string FirstPart(string relative) =>
        Normalize(relative).Split(Path.DirectorySeparatorChar, 2)[0];

    private static bool IsSimpleFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) && Path.GetFileName(value) == value &&
        value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static void CopyAtomic(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".beamsplit-new";
        try
        {
            File.Copy(source, temp, true);
            File.Move(temp, destination, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static void CleanOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        try { Directory.Delete(path, true); } catch { }
    }

    private static void TryDelete(string path, IProgress<string>? log)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log?.Report($"Server mods: could not remove {Path.GetFileName(path)} - {ex.Message}"); }
    }
}

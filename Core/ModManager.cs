using System.Diagnostics;
using System.IO;

namespace BeamSplit.Core;

public sealed record ModPackage(string RelativePath, string FullPath, long Bytes)
{
    public string Name => Path.GetFileName(RelativePath);
}

/// <summary>
/// Mounts one existing mod library into each player profile without copying it, and
/// synchronises user-selected ZIPs into the BeamMP server. BeamSplit never writes to
/// the mounted source. The pinned BeamMP.zip remains in mods/multiplayer, outside the
/// shared library.
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
        var repo = Path.Combine(mods, "repo");
        // Repository downloads normally live here. Mounting the parent mods folder
        // would also expose multiplayer/BeamMP.zip to every profile a second time.
        if (Directory.Exists(repo)) return repo;
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
                log?.Report("Mods: source folder is unavailable; existing player links and server packages were left in place.");
            return;
        }
        var discovered = Discover(cfg.ModsSourceDir)
            .ToDictionary(m => Normalize(m.RelativePath), StringComparer.OrdinalIgnoreCase);
        SyncPlayers(cfg, Math.Max(0, playerCount), log);
        SyncServer(cfg, discovered, log);
    }

    private static void SyncPlayers(AppConfig cfg, int playerCount, IProgress<string>? log)
    {
        var source = ResolvePlayerSource(cfg.ModsSourceDir!);
        var linked = 0;
        for (var i = 0; i < playerCount; i++)
        {
            var target = Path.Combine(Instances.CurrentProfile(cfg, i), "mods", PlayerFolderName);
            CleanOwnedDirectory(target);
            if (!cfg.UsePlayerMods) continue;
            try
            {
                CreateJunction(target, source);
                linked++;
            }
            catch (Exception ex) { log?.Report($"Personal mods: could not link the library to P{i} - {ex.Message}"); }
        }
        log?.Report(cfg.UsePlayerMods
            ? $"Personal mods: shared {source} with {linked}/{playerCount} player profile(s), zero copies."
            : "Personal mods: shared-library links disabled and cleared.");
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
        var info = new DirectoryInfo(path);
        try
        {
            info.Refresh();
            if (info.LinkTarget != null)
            {
                Directory.Delete(path);
                return;
            }
        }
        catch { }
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        try { Directory.Delete(path, true); } catch { }
    }

    /// <summary>
    /// If somebody points at the whole mods folder, narrow the player mount to repo so
    /// multiplayer downloads cannot appear through the shared link. Server discovery
    /// can still see eligible ZIPs elsewhere in the chosen source.
    /// </summary>
    internal static string ResolvePlayerSource(string source)
    {
        var root = Path.GetFullPath(source);
        if (Directory.Exists(Path.Combine(root, "multiplayer")))
        {
            var repo = Path.Combine(root, "repo");
            if (Directory.Exists(repo)) return repo;
            throw new InvalidOperationException("Choose a mod library that does not contain the multiplayer folder.");
        }
        return root;
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the Windows junction tool.");
        if (!process.WaitForExit(15000))
        {
            try { process.Kill(true); } catch { }
            throw new IOException("Timed out while creating the shared-library junction.");
        }
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException(process.StandardError.ReadToEnd().Trim() is { Length: > 0 } error
                ? error
                : "Windows could not create the shared-library junction.");
    }

    private static void TryDelete(string path, IProgress<string>? log)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log?.Report($"Server mods: could not remove {Path.GetFileName(path)} - {ex.Message}"); }
    }
}

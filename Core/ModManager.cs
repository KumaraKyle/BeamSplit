using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

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
    public const string RepositoryFolderName = "beamsplit-repository";
    public const string RepositoryKeyPrefix = "Official Repository";
    public static string RepositorySource => Path.Combine(Paths.ModsDir, "repository");

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

    public static IReadOnlyList<ModPackage> DiscoverConfigured(AppConfig cfg, string? sourceOverride = null)
    {
        var source = sourceOverride ?? cfg.ModsSourceDir;
        var packages = new List<ModPackage>();
        if (!string.IsNullOrWhiteSpace(source) && Directory.Exists(source))
            packages.AddRange(Discover(source));

        var sourceFull = !string.IsNullOrWhiteSpace(source) && Directory.Exists(source)
            ? Path.GetFullPath(source)
            : null;
        if (Directory.Exists(RepositorySource) &&
            !string.Equals(sourceFull, Path.GetFullPath(RepositorySource), StringComparison.OrdinalIgnoreCase))
        {
            packages.AddRange(Discover(RepositorySource).Select(package => new ModPackage(
                Path.Combine(RepositoryKeyPrefix, package.RelativePath),
                package.FullPath,
                package.Bytes)));
        }
        return packages.OrderBy(package => package.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Apply(AppConfig cfg, int playerCount, IProgress<string>? log = null)
    {
        var personalAvailable = !string.IsNullOrWhiteSpace(cfg.ModsSourceDir) && Directory.Exists(cfg.ModsSourceDir);
        var repositoryAvailable = Directory.Exists(RepositorySource) && Discover(RepositorySource).Count > 0;
        if (!personalAvailable && !repositoryAvailable)
        {
            SyncPlayers(cfg, Math.Max(0, playerCount), log);
            if (cfg.ModsConfigured)
                log?.Report("Mods: source folder is unavailable; stale player links were cleared and existing server packages were left in place.");
            return;
        }
        var discovered = DiscoverConfigured(cfg)
            .ToDictionary(m => Normalize(m.RelativePath), StringComparer.OrdinalIgnoreCase);
        SyncPlayers(cfg, Math.Max(0, playerCount), log);
        SyncServer(cfg, discovered, log);
    }

    private static void SyncPlayers(AppConfig cfg, int playerCount, IProgress<string>? log)
    {
        var personalSource = !string.IsNullOrWhiteSpace(cfg.ModsSourceDir) && Directory.Exists(cfg.ModsSourceDir)
            ? ResolvePlayerSource(cfg.ModsSourceDir)
            : null;
        var repositoryReady = Directory.Exists(RepositorySource) && Discover(RepositorySource).Count > 0;
        var personalLinked = 0;
        var repositoryLinked = 0;
        for (var i = 0; i < playerCount; i++)
        {
            var personalTarget = Path.Combine(Instances.CurrentProfile(cfg, i), "mods", PlayerFolderName);
            var repositoryTarget = Path.Combine(Instances.CurrentProfile(cfg, i), "mods", RepositoryFolderName);
            CleanOwnedDirectory(personalTarget);
            CleanOwnedDirectory(repositoryTarget);
            if (cfg.UsePlayerMods && personalSource is not null)
            {
                try
                {
                    CreateJunction(personalTarget, personalSource);
                    personalLinked++;
                }
                catch (Exception ex) { log?.Report($"Personal mods: could not link the library to P{i} - {ex.Message}"); }
            }
            if (cfg.UseRepositoryMods && repositoryReady)
            {
                try
                {
                    CreateJunction(repositoryTarget, RepositorySource);
                    repositoryLinked++;
                }
                catch (Exception ex) { log?.Report($"Official repository mods: could not link the library to P{i} - {ex.Message}"); }
            }
        }
        log?.Report(cfg.UsePlayerMods
            ? $"Personal mods: shared {personalSource ?? "(source unavailable)"} with {personalLinked}/{playerCount} player profile(s), zero copies."
            : "Personal mods: shared-library links disabled and cleared.");
        log?.Report(cfg.UseRepositoryMods
            ? $"Official repository mods: linked to {repositoryLinked}/{playerCount} player profile(s)."
            : "Official repository mods: profile links disabled and cleared.");
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
            if (wanted.Any(relative => SafeServerFileName(Path.GetFileName(relative))
                    .Equals(old, StringComparison.OrdinalIgnoreCase))) continue;
            TryDelete(Path.Combine(client, old), log);
        }

        var nowManaged = new List<string>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in wanted.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!discovered.TryGetValue(relative, out var mod)) continue;
            var sourceName = Path.GetFileName(relative);
            var name = SafeServerFileName(sourceName);
            if (!usedNames.Add(name))
            {
                log?.Report($"Server mods: skipped {relative}; another selected ZIP resolves to {name}.");
                continue;
            }

            var issue = InspectServerPackage(mod.FullPath);
            if (issue is not null)
            {
                log?.Report($"Server mods: skipped {sourceName} - {issue}");
                continue;
            }

            if (!name.Equals(sourceName, StringComparison.Ordinal))
                log?.Report($"Server mods: normalized {sourceName} to {name} for BeamMP UTF-8 compatibility.");

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

    /// <summary>
    /// BeamMP Server 3.x serializes resource names through a narrow string on Windows.
    /// A typographic dash or other ANSI-only character can consequently become byte
    /// 0x97 in mods.json and make the entire mod database unreadable. Managed copies
    /// therefore get a deterministic ASCII filename; the source package is untouched.
    /// </summary>
    internal static string SafeServerFileName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var output = new StringBuilder(stem.Length);
        var underscore = false;
        foreach (var c in stem)
        {
            var safe = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
            if (safe)
            {
                output.Append(c);
                underscore = false;
            }
            else if (!underscore)
            {
                output.Append('_');
                underscore = true;
            }
        }
        var clean = output.ToString().Trim('.', '_', '-');
        if (clean.Length == 0) clean = "beamsplit-mod";
        return clean + ".zip";
    }

    /// <summary>Returns a user-facing reason when BeamMP cannot safely index a ZIP.</summary>
    internal static string? InspectServerPackage(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var strictUtf8 = new UTF8Encoding(false, true);
            foreach (var entry in archive.Entries.Where(e =>
                         e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                if (entry.Length > 16 * 1024 * 1024)
                    return $"{entry.FullName} is an unexpectedly large JSON file";
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                string json;
                try { json = strictUtf8.GetString(memory.ToArray()); }
                catch (DecoderFallbackException)
                {
                    return $"{entry.FullName} is not UTF-8 encoded";
                }

                if (entry.FullName.Contains("mod_info/", StringComparison.OrdinalIgnoreCase))
                {
                    try { using var _ = JsonDocument.Parse(json); }
                    catch (JsonException) { return $"{entry.FullName} contains invalid JSON"; }
                }
            }
            return null;
        }
        catch (InvalidDataException) { return "the file is not a readable ZIP archive"; }
        catch (IOException ex) { return $"the ZIP could not be read ({ex.Message})"; }
        catch (UnauthorizedAccessException) { return "access to the ZIP was denied"; }
    }

    internal static IReadOnlyList<string> InspectServerDirectory(AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ServerDir)) return [];
        var client = Path.Combine(cfg.ServerDir, "Resources", "Client");
        if (!Directory.Exists(client)) return [];
        var issues = new List<string>();
        foreach (var path in Directory.GetFiles(client, "*.zip", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.All(c => c <= 0x7f))
                issues.Add($"{name}: filename contains non-ASCII characters");
            var issue = InspectServerPackage(path);
            if (issue is not null) issues.Add($"{name}: {issue}");
        }
        return issues;
    }

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

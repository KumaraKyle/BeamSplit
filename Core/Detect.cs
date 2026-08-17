using System.IO;
using System.Text.RegularExpressions;

namespace BeamSplit.Core;

/// <summary>Finds BeamNG, the BeamMP launcher and a BeamMP server without asking the user.</summary>
public static partial class Detect
{
    private const string GameExeRel = @"Bin64\BeamNG.drive.x64.exe";

    public static void FillMissing(AppConfig cfg)
    {
        cfg.GameRoot ??= FindBeamNG();
        cfg.LauncherExe ??= FindLauncher();
        cfg.ServerDir ??= FindServer();
    }

    public static bool IsGameRoot(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, GameExeRel));

    /// <summary>
    /// Every BeamNG install we can find, best guess first.
    ///
    /// More than one is common and matters: BeamNG rewrites installPath in its ini
    /// every time a copy is launched, so "the" install can flip between runs. This
    /// machine has both B:\BeamNG.drive and B:\Games\BeamNG.drive. Instances are
    /// built as junctions into a specific root, so silently switching would point
    /// them at the wrong game.
    /// </summary>
    public static List<string> FindAllBeamNG()
    {
        var found = new List<string>();
        void Add(string? p)
        {
            if (IsGameRoot(p) && !found.Any(f => string.Equals(f, p, StringComparison.OrdinalIgnoreCase)))
                found.Add(p!);
        }

        Add(FindBeamNG());   // the ini / Steam / scan winner goes first

        foreach (var lib in SteamLibraries())
            Add(Path.Combine(lib, "steamapps", "common", "BeamNG.drive"));

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            var root = d.RootDirectory.FullName;
            Add(Path.Combine(root, "Games", "BeamNG.drive"));
            Add(Path.Combine(root, "BeamNG.drive"));
            Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "BeamNG.drive"));
        }
        return found;
    }

    public static string? FindBeamNG()
    {
        // 1. the ini BeamNG maintains itself - most reliable, and it is also where
        //    the installed version comes from
        var ini = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG", "BeamNG.drive.ini");
        if (File.Exists(ini))
        {
            foreach (var line in File.ReadLines(ini))
            {
                var m = InstallPathRe().Match(line);
                if (m.Success)
                {
                    var p = m.Groups[1].Value.Trim().TrimEnd('\\');
                    if (IsGameRoot(p)) return p;
                }
            }
        }

        // 2. Steam libraries
        foreach (var lib in SteamLibraries())
        {
            var p = Path.Combine(lib, "steamapps", "common", "BeamNG.drive");
            if (IsGameRoot(p)) return p;
        }

        // 3. common spots on each fixed drive
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            foreach (var guess in new[]
                     {
                         Path.Combine(d.RootDirectory.FullName, "Games", "BeamNG.drive"),
                         Path.Combine(d.RootDirectory.FullName, "BeamNG.drive"),
                         Path.Combine(d.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "BeamNG.drive")
                     })
            {
                if (IsGameRoot(guess)) return guess;
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        foreach (var baseDir in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
        {
            var steam = Path.Combine(baseDir, "Steam");
            if (!Directory.Exists(steam)) continue;
            roots.Add(steam);

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in LibraryPathRe().Matches(File.ReadAllText(vdf)))
                roots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return roots.Distinct();
    }

    public static string? FindLauncher()
    {
        foreach (var p in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "BeamMP-Launcher", "BeamMP-Launcher.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                  "BeamMP-Launcher", "BeamMP-Launcher.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                  "BeamMP-Launcher", "BeamMP-Launcher.exe")
                 })
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string? FindServer()
    {
        if (File.Exists(Path.Combine(Paths.ServerDirDefault, "BeamMP-Server.exe")))
            return Paths.ServerDirDefault;

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            foreach (var guess in new[]
                     {
                         Path.Combine(d.RootDirectory.FullName, "Games", "BeamMP-Server"),
                         Path.Combine(d.RootDirectory.FullName, "BeamMP-Server")
                     })
            {
                if (File.Exists(Path.Combine(guess, "BeamMP-Server.exe"))) return guess;
            }
        }
        return null;
    }

    /// <summary>Installed BeamNG version, e.g. "0.38.5.0". Null if it can't be determined.</summary>
    public static string? GameVersion(AppConfig cfg)
    {
        var ini = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BeamNG", "BeamNG.drive.ini");
        if (File.Exists(ini))
        {
            foreach (var line in File.ReadLines(ini))
            {
                var m = VersionRe().Match(line);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }

        if (IsGameRoot(cfg.GameRoot))
        {
            var exe = Path.Combine(cfg.GameRoot!, GameExeRel);
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion;
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    /// <summary>
    /// The middle number of the version - "0.38.5.0" gives 38. This is what BeamMP's
    /// client compares against in its compatibleVersion check.
    /// </summary>
    public static int GameMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var m = MajorRe().Match(version);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    [GeneratedRegex(@"^\s*installPath\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallPathRe();
    [GeneratedRegex(@"^\s*version\s*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRe();
    [GeneratedRegex(@"^\d+\.(\d+)")]
    private static partial Regex MajorRe();
    [GeneratedRegex("\"path\"\\s*\"([^\"]+)\"")]
    private static partial Regex LibraryPathRe();
}
